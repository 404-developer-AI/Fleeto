using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The managed endpoints of a scope per status in the newest report.</summary>
public sealed record EndOfLifeCounts(int Supported, int EndingSoon, int Extended, int EndOfLife, int NoDate, int NotRecognised)
{
    public static readonly EndOfLifeCounts None = new(0, 0, 0, 0, 0, 0);

    public int Total => Supported + EndingSoon + Extended + EndOfLife + NoDate + NotRecognised;
}

/// <summary>One endpoint of a report that is not supported for long any more.</summary>
/// <param name="ExtensionMarked">The endpoint carries the mark of a paid extension for the release it ran in the report.</param>
/// <param name="ExtensionPossible">A paid extension exists for that release, so the mark can be set.</param>
public sealed record EndOfLifeRowView(Guid EndpointId, Guid ClientId, string ClientCode, string ClientName, string SiteName, string Hostname, string OsName,
    EndOfLifeProduct? Product, string? Release, string? ReleaseLabel, EndOfLifeStatus Status, DateOnly? EndOfLifeOn, DateOnly? ExtensionUntil,
    bool ExtensionCounted, bool ExtensionMarked, bool ExtensionPossible);

/// <summary>Where the dates of one product come from and how old they are.</summary>
public sealed record EndOfLifeSourceView(EndOfLifeProduct Product, string Name, string SourceName, string SourceLink, bool Builtin, DateTime? LastSuccessAt,
    DateTime? LastChangedAt, int Releases, string? Problem, DateTime? FailingSince);

/// <summary>The newest report of a scope as the tab End of life shows it; <see cref="ReportId"/> is null before the first run.</summary>
public sealed record EndOfLifeOverview(Guid? ReportId, DateTime? CreatedAt, bool Scheduled, string? RequestedBy, DateOnly? Day, int PeriodMonths,
    DateTime? DataAsOf, EndOfLifeCounts Counts, IReadOnlyList<EndOfLifeRowView> Rows, IReadOnlyList<EndOfLifeSourceView> Sources,
    EndOfLifeSchedule Schedule, DateTime? NextRun, bool RefreshPending);

/// <summary>The report of one client, for its PDF.</summary>
public sealed record EndOfLifeClientReport(string ClientCode, string ClientName, DateTime CreatedAt, DateOnly Day, int PeriodMonths, DateTime? DataAsOf,
    EndOfLifeCounts Counts, IReadOnlyList<EndOfLifeRowView> Rows, string GeneratedBy, DateTime GeneratedAt)
{
    public string FileName => $"End of life {ClientCode} {Day:yyyy-MM-dd}.pdf";
}

/// <summary>Settings, Vulnerabilities: the schedule, the period and the sources.</summary>
public sealed record EndOfLifeSettingsView(EndOfLifeSchedule Schedule, int PeriodMonths, DateTime? NextRun, IReadOnlyList<EndOfLifeSourceView> Sources);

/// <summary>
/// The end-of-life report on the web (0.8.0 step 4, VULNERABILITIES.md §12): the tab End of life under Vulnerabilities, "Refresh now",
/// the mark of a paid extension, the PDF per client and the settings. Reads go through the client scope of the caller; the report itself
/// is written by the workers, which also reach endoflife.date. Viewing is not audited; a refresh, a mark, a setting and an export are.
/// </summary>
public sealed class EndOfLifeReportService
{
    /// <summary>The rows the tab lists; a scope with more shows the most urgent.</summary>
    public const int MaxRows = 5000;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public EndOfLifeReportService(IFleetoDbContextFactory dbFactory, SettingsStore settings, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _time = time;
    }

    /// <summary>The newest report for all clients, a client or a site.</summary>
    public async Task<EndOfLifeOverview> GetOverviewAsync(Caller caller, Guid? clientId, Guid? siteId, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.VulnerabilitiesView);
        var now = _time.GetUtcNow().UtcDateTime;
        var schedule = EndOfLife.ParseSchedule(await _settings.GetStringAsync(SettingKeys.EndOfLifeSchedule, cancellationToken));
        var period = EndOfLife.PeriodMonths(await _settings.GetStringAsync(SettingKeys.EndOfLifePeriodMonths, cancellationToken));
        await using var db = _dbFactory.Create(caller.Scope);
        var sources = await SourcesAsync(db, cancellationToken);
        var pending = await db.EndOfLifeRefreshRequests.AsNoTracking().AnyAsync(r => r.HandledAt == null, cancellationToken);
        var nextRun = await NextRunAsync(db, schedule, cancellationToken);
        var report = await db.EndOfLifeReports.AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return new EndOfLifeOverview(null, null, false, null, null, period, null, EndOfLifeCounts.None, [], sources, schedule, nextRun, pending);
        }

        var rows = await RowsAsync(db, report.Id, clientId, siteId, cancellationToken);
        var counts = await CountsAsync(db, report.Id, clientId, siteId, rows, cancellationToken);
        return new EndOfLifeOverview(report.Id, report.CreatedAt, report.Scheduled, report.RequestedByName, report.Day, report.PeriodMonths, report.DataAsOf,
            counts, rows, sources, schedule, nextRun, pending);
    }

    /// <summary>
    /// The rows of a report in a scope, with the current mark of each endpoint. The client scope applies through the context, so a user
    /// limited to part of a client sees only its own endpoints.
    /// </summary>
    private static async Task<List<EndOfLifeRowView>> RowsAsync(FleetoDbContext db, Guid reportId, Guid? clientId, Guid? siteId,
        CancellationToken cancellationToken)
    {
        var query = db.EndOfLifeReportRows.AsNoTracking().Where(r => r.ReportId == reportId);
        if (clientId is { } client)
        {
            query = query.Where(r => r.ClientId == client);
        }

        if (siteId is { } site)
        {
            query = query.Where(r => r.SiteId == site);
        }

        var rows = await (
                from r in query
                join c in db.Clients.AsNoTracking() on r.ClientId equals c.Id
                join s in db.Sites.AsNoTracking() on r.SiteId equals s.Id into sites
                from s in sites.DefaultIfEmpty()
                join e in db.Endpoints.AsNoTracking() on r.EndpointId equals e.Id
                orderby r.Status == EndOfLifeStatus.EndOfLife ? 0 : r.Status == EndOfLifeStatus.EndingSoon ? 1 : r.Status == EndOfLifeStatus.Extended ? 2 : 3,
                    r.EndOfLifeOn, c.Code, r.Hostname
                select new
                {
                    r.EndpointId, r.ClientId, c.Code, c.Name, SiteName = s != null ? s.Name : string.Empty, r.Hostname, r.OsName, r.Product, r.Release,
                    r.ReleaseLabel, r.Status, r.EndOfLifeOn, r.ExtensionUntil, r.ExtensionCounted, e.EndOfLifeExtensionRelease
                })
            .Take(MaxRows)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new EndOfLifeRowView(r.EndpointId, r.ClientId, r.Code, r.Name, r.SiteName, r.Hostname, r.OsName, r.Product, r.Release,
                r.ReleaseLabel, r.Status, r.EndOfLifeOn, r.ExtensionUntil, r.ExtensionCounted,
                r.Release is not null && r.EndOfLifeExtensionRelease == r.Release, r.ExtensionUntil is not null))
            .ToList();
    }

    /// <summary>
    /// The counts of a scope. The statuses other than supported come from the rows the caller sees, so they are exact for every scope;
    /// supported endpoints have no row and come from the counts per site, which a user with part of a site does not see.
    /// </summary>
    private static async Task<EndOfLifeCounts> CountsAsync(FleetoDbContext db, Guid reportId, Guid? clientId, Guid? siteId, List<EndOfLifeRowView> rows,
        CancellationToken cancellationToken)
    {
        var counts = db.EndOfLifeReportCounts.AsNoTracking().Where(c => c.ReportId == reportId && c.Status == EndOfLifeStatus.Supported);
        if (clientId is { } client)
        {
            counts = counts.Where(c => c.ClientId == client);
        }

        if (siteId is { } site)
        {
            counts = counts.Where(c => c.SiteId == site);
        }

        var supported = await counts.SumAsync(c => c.Endpoints, cancellationToken);
        return new EndOfLifeCounts(supported,
            rows.Count(r => r.Status == EndOfLifeStatus.EndingSoon),
            rows.Count(r => r.Status == EndOfLifeStatus.Extended),
            rows.Count(r => r.Status == EndOfLifeStatus.EndOfLife),
            rows.Count(r => r.Status == EndOfLifeStatus.NoDate),
            rows.Count(r => r.Status == EndOfLifeStatus.NotRecognised));
    }

    private static async Task<List<EndOfLifeSourceView>> SourcesAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var states = await db.EndOfLifeSources.AsNoTracking().ToDictionaryAsync(s => s.Product, cancellationToken);
        return EndOfLife.Products.Select(p =>
        {
            var builtin = p.Source == EndOfLifeSourceKind.Builtin;
            var state = states.GetValueOrDefault(p.Product);
            return new EndOfLifeSourceView(p.Product, p.Name, p.SourceName, p.SourceLink, builtin, state?.LastSuccessAt, state?.LastChangedAt,
                builtin ? EndOfLife.SynologyDsmReleases.Count : state?.ReleaseCount ?? 0, state?.Problem, state?.FailingSince);
        }).ToList();
    }

    private static async Task<DateTime?> NextRunAsync(FleetoDbContext db, EndOfLifeSchedule schedule, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.EndOfLifeSchedule).Select(s => (DateTime?)s.UpdatedAt)
            .SingleOrDefaultAsync(cancellationToken);
        var last = await db.EndOfLifeReports.AsNoTracking().Where(r => r.Scheduled).MaxAsync(r => (DateTime?)r.CreatedAt, cancellationToken);
        var from = (last, setting) switch
        {
            ({ } l, { } s) => l > s ? l : s,
            ({ } l, null) => l,
            (null, { } s) => s,
            _ => DateTime.UtcNow
        };
        return EndOfLife.NextRun(schedule, from);
    }

    /// <summary>
    /// Asks the workers for a new report now; a database trigger wakes them. Once a minute for the instance, so a busy page cannot keep
    /// the workers fetching.
    /// </summary>
    public async Task<ServiceResult> RequestRefreshAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.VulnerabilitiesView))
        {
            return ServiceResult.Forbidden();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var last = await db.EndOfLifeRefreshRequests.AsNoTracking().MaxAsync(r => (DateTime?)r.RequestedAt, cancellationToken);
        if (last is { } at && now - at < EndOfLife.RefreshCooldown)
        {
            return ServiceResult.Fail("The report was refreshed less than a minute ago. Wait a moment and try again.");
        }

        db.EndOfLifeRefreshRequests.Add(new EndOfLifeRefreshRequest { Id = Guid.NewGuid(), RequestedAt = now, UserId = caller.UserId, UserName = caller.Name });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndOfLifeRefreshRequested, "EndOfLifeReport", "all", null, null), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Marks that the client bought a paid extension for the release the endpoint runs now, or removes the mark. Only for a managed
    /// endpoint whose release has an extension; the report changes with the next run, which this asks for at once.
    /// </summary>
    public async Task<ServiceResult> SetExtensionAsync(Caller caller, Guid endpointId, bool marked, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.EndpointsEdit))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult.NotFound("endpoint");
        }

        string? release = null;
        if (marked)
        {
            if (endpoint.Tier != EndpointTier.Managed)
            {
                return ServiceResult.Fail("The end-of-life report covers managed endpoints only. Switch the endpoint to managed first.");
            }

            var releases = (await db.EndOfLifeReleases.AsNoTracking().ToListAsync(cancellationToken)).Select(r => r.ToRelease())
                .Concat(EndOfLife.SynologyDsmReleases).ToList();
            var product = EndOfLife.ProductOf(endpoint.OsPlatform, endpoint.OsName);
            var current = product is { } p ? EndOfLife.ReleaseOf(p, endpoint.OsName, endpoint.OsVersion, releases) : null;
            if (current?.ExtensionUntil is null)
            {
                return ServiceResult.Fail($"{endpoint.OsName} has no paid extension Fleeto knows of, so there is nothing to mark.");
            }

            release = current.Release;
        }

        if (endpoint.EndOfLifeExtensionRelease == release)
        {
            return ServiceResult.Ok();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var before = endpoint.EndOfLifeExtensionRelease;
        endpoint.EndOfLifeExtensionRelease = release;
        endpoint.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndOfLifeExtensionChanged, "Endpoint", endpoint.Id.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, Before = before, After = release }), now));
        db.EndOfLifeRefreshRequests.Add(new EndOfLifeRefreshRequest { Id = Guid.NewGuid(), RequestedAt = now, UserId = caller.UserId, UserName = caller.Name });
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The newest report of one client, for its PDF. Audited without its content.</summary>
    public async Task<ServiceResult<EndOfLifeClientReport>> GetClientReportAsync(Caller caller, Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.VulnerabilitiesView))
        {
            return ServiceResult<EndOfLifeClientReport>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var client = await db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => new { c.Id, c.Code, c.Name }).SingleOrDefaultAsync(cancellationToken);
        if (client is null)
        {
            return ServiceResult<EndOfLifeClientReport>.NotFound("client");
        }

        var report = await db.EndOfLifeReports.AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return ServiceResult<EndOfLifeClientReport>.Fail("There is no report yet. Refresh the report first.");
        }

        var rows = await RowsAsync(db, report.Id, clientId, null, cancellationToken);
        var counts = await CountsAsync(db, report.Id, clientId, null, rows, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndOfLifeReportExported, "Client", client.Id.ToString(), client.Id,
            new { Report = report.Id, Endpoints = rows.Count }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<EndOfLifeClientReport>.Ok(new EndOfLifeClientReport(client.Code, client.Name, report.CreatedAt, report.Day, report.PeriodMonths,
            report.DataAsOf, counts, rows, caller.Name, now));
    }

    /// <summary>Settings, Vulnerabilities: the schedule, the period and the state of every source.</summary>
    public async Task<EndOfLifeSettingsView> GetSettingsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.VulnerabilitiesManage);
        var schedule = EndOfLife.ParseSchedule(await _settings.GetStringAsync(SettingKeys.EndOfLifeSchedule, cancellationToken));
        var period = EndOfLife.PeriodMonths(await _settings.GetStringAsync(SettingKeys.EndOfLifePeriodMonths, cancellationToken));
        await using var db = _dbFactory.CreateSystem();
        return new EndOfLifeSettingsView(schedule, period, await NextRunAsync(db, schedule, cancellationToken), await SourcesAsync(db, cancellationToken));
    }

    /// <summary>Saves the schedule and the period. A changed schedule starts from now, so it never runs for a moment in the past.</summary>
    public async Task<ServiceResult> SaveSettingsAsync(Caller caller, EndOfLifeSchedule schedule, int periodMonths, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.VulnerabilitiesManage))
        {
            return ServiceResult.Forbidden();
        }

        if (EndOfLife.ScheduleProblem(schedule) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        if (periodMonths is < EndOfLife.MinPeriodMonths or > EndOfLife.MaxPeriodMonths)
        {
            return ServiceResult.Fail($"Choose a period between {EndOfLife.MinPeriodMonths} and {EndOfLife.MaxPeriodMonths} months.");
        }

        var beforeSchedule = EndOfLife.ParseSchedule(await _settings.GetStringAsync(SettingKeys.EndOfLifeSchedule, cancellationToken));
        var beforePeriod = EndOfLife.PeriodMonths(await _settings.GetStringAsync(SettingKeys.EndOfLifePeriodMonths, cancellationToken));
        if (beforeSchedule == schedule && beforePeriod == periodMonths)
        {
            return ServiceResult.Ok();
        }

        if (beforeSchedule != schedule)
        {
            await _settings.SetStringAsync(SettingKeys.EndOfLifeSchedule, EndOfLife.ScheduleJson(schedule), false, caller.UserId, cancellationToken);
        }

        if (beforePeriod != periodMonths)
        {
            await _settings.SetStringAsync(SettingKeys.EndOfLifePeriodMonths, periodMonths.ToString(CultureInfo.InvariantCulture), false, caller.UserId,
                cancellationToken);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.EndOfLifeSettingsChanged, "Setting", "end-of-life", null,
            new
            {
                Before = new { Schedule = EndOfLife.Describe(beforeSchedule), PeriodMonths = beforePeriod },
                After = new { Schedule = EndOfLife.Describe(schedule), PeriodMonths = periodMonths }
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }
}
