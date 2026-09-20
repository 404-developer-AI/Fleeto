using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Api;

public sealed record ApiEndpointFilter(Guid? ClientId, Guid? SiteId, EndpointClass? Class, EndpointTier? Tier, bool? Online, string? Search);

/// <param name="State">Null: every state.</param>
public sealed record ApiAlertFilter(AlertStateFilter State, AlertSeverity? Severity, Guid? ClientId, Guid? EndpointId);

public sealed record ApiJobFilter(Guid? ClientId, Guid? EndpointId, JobState? State);

/// <summary>A read of something that exists only on managed endpoints: <see cref="Managed"/> false means the endpoint is agent-only.</summary>
public sealed record ManagedRead<T>(bool Managed, T? Value);

/// <summary>
/// The reads of the public API. Every query runs in a context created with the key's client scope, so the global query filters hide other
/// clients exactly as they do in the UI; tier and maintenance use the same domain rules. Where a UI service already returns the right data
/// (inventory, checks, job output) it is reused rather than rewritten.
/// </summary>
public sealed class PublicApiQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxSearchLength = 100;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly EndpointService _endpoints;
    private readonly EndpointCheckService _checks;
    private readonly JobService _jobs;
    private readonly TimeProvider _time;

    public PublicApiQueries(IFleetoDbContextFactory dbFactory, LicenseService licenses, EndpointService endpoints, EndpointCheckService checks,
        JobService jobs, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _endpoints = endpoints;
        _checks = checks;
        _jobs = jobs;
        _time = time;
    }

    // Clients

    /// <summary>Ordered by code (unique), keyset on (Code, Id).</summary>
    public async Task<ApiPage<ApiClient>> ListClientsAsync(Caller caller, string? search, string? cursor, int limit, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var clients = db.Clients.AsNoTracking();
        if (ServiceSupport.Clean(search) is { } term)
        {
            var pattern = ServiceSupport.LikePattern(term);
            clients = clients.Where(c => EF.Functions.ILike(c.Code, pattern) || EF.Functions.ILike(c.Name, pattern));
        }

        if (cursor is not null && ApiCursor.Require(cursor, out string code, out var id))
        {
            clients = clients.Where(c => EF.Functions.GreaterThan(ValueTuple.Create(c.Code, c.Id), ValueTuple.Create(code, id)));
        }

        var rows = await ProjectClients(db, clients.OrderBy(c => c.Code).ThenBy(c => c.Id).Take(limit + 1)).ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        return Page(rows.Select(r => ToApi(r, now)).ToList(), limit, c => ApiCursor.Encode(c.Code, c.Id));
    }

    public async Task<ApiClient?> GetClientAsync(Caller caller, Guid clientId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var row = await ProjectClients(db, db.Clients.AsNoTracking().Where(c => c.Id == clientId)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToApi(row, _time.GetUtcNow().UtcDateTime);
    }

    private sealed record ClientRow(Client Client, int SiteCount, int EndpointCount);

    private static IQueryable<ClientRow> ProjectClients(FleetoDbContext db, IQueryable<Client> clients) =>
        clients.Select(c => new ClientRow(c, db.Sites.Count(s => s.ClientId == c.Id), db.Endpoints.Count(e => e.ClientId == c.Id)));

    private static ApiClient ToApi(ClientRow row, DateTime now) =>
        new(row.Client.Id, row.Client.Code, row.Client.Name, row.SiteCount, row.EndpointCount, Maintenance(row.Client.Maintenance, now),
            Utc(row.Client.CreatedAt), Utc(row.Client.UpdatedAt));

    // Sites

    /// <summary>Ordered by name, keyset on (Name, Id).</summary>
    public async Task<ApiPage<ApiSite>> ListSitesAsync(Caller caller, Guid? clientId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var sites = db.Sites.AsNoTracking();
        if (clientId is { } client)
        {
            sites = sites.Where(s => s.ClientId == client);
        }

        if (cursor is not null && ApiCursor.Require(cursor, out string name, out var id))
        {
            sites = sites.Where(s => EF.Functions.GreaterThan(ValueTuple.Create(s.Name, s.Id), ValueTuple.Create(name, id)));
        }

        var rows = await sites.OrderBy(s => s.Name).ThenBy(s => s.Id).Take(limit + 1)
            .Select(s => new { Site = s, Endpoints = db.Endpoints.Count(e => e.SiteId == s.Id) })
            .ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        return Page(rows.Select(r => ToApi(r.Site, r.Endpoints, now)).ToList(), limit, s => ApiCursor.Encode(s.Name, s.Id));
    }

    public async Task<ApiSite?> GetSiteAsync(Caller caller, Guid siteId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var row = await db.Sites.AsNoTracking().Where(s => s.Id == siteId)
            .Select(s => new { Site = s, Endpoints = db.Endpoints.Count(e => e.SiteId == s.Id) })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToApi(row.Site, row.Endpoints, _time.GetUtcNow().UtcDateTime);
    }

    private static ApiSite ToApi(Site site, int endpointCount, DateTime now) =>
        new(site.Id, site.ClientId, site.Name, site.Description, endpointCount, Maintenance(site.Maintenance, now), Utc(site.CreatedAt), Utc(site.UpdatedAt));

    // Endpoints

    /// <summary>Ordered by hostname, keyset on (Hostname, Id).</summary>
    public async Task<ApiPage<ApiEndpoint>> ListEndpointsAsync(Caller caller, ApiEndpointFilter filter, string? cursor, int limit,
        CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoints = db.Endpoints.AsNoTracking();
        if (filter.ClientId is { } clientId)
        {
            endpoints = endpoints.Where(e => e.ClientId == clientId);
        }

        if (filter.SiteId is { } siteId)
        {
            endpoints = endpoints.Where(e => e.SiteId == siteId);
        }

        if (filter.Class is { } endpointClass)
        {
            endpoints = endpoints.Where(e => (e.ClassOverride ?? e.DetectedClass) == endpointClass);
        }

        if (filter.Tier is { } tier)
        {
            endpoints = endpoints.Where(e => e.Tier == tier);
        }

        if (filter.Online is { } online)
        {
            endpoints = endpoints.Where(e => e.IsOnline == online);
        }

        if (ServiceSupport.Clean(filter.Search) is { } term)
        {
            endpoints = endpoints.Where(e => EF.Functions.ILike(e.Hostname, ServiceSupport.LikePattern(term)));
        }

        if (cursor is not null && ApiCursor.Require(cursor, out string hostname, out var id))
        {
            endpoints = endpoints.Where(e => EF.Functions.GreaterThan(ValueTuple.Create(e.Hostname, e.Id), ValueTuple.Create(hostname, id)));
        }

        var rows = await ToApiEndpointsAsync(db, endpoints.OrderBy(e => e.Hostname).ThenBy(e => e.Id).Take(limit + 1), cancellationToken);
        return Page(rows, limit, e => ApiCursor.Encode(e.Hostname, e.Id));
    }

    public async Task<ApiEndpoint?> GetEndpointAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return (await ToApiEndpointsAsync(db, db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId), cancellationToken)).SingleOrDefault();
    }

    private async Task<List<ApiEndpoint>> ToApiEndpointsAsync(FleetoDbContext db, IQueryable<Endpoint> endpoints, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // Open alert counts leave out alerts on hold, as in the UI.
        var rows = await endpoints.Select(e => new
            {
                Endpoint = e,
                OpenAlerts = db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved && (a.HeldUntil == null || a.HeldUntil <= now)),
                HeldAlerts = db.Alerts.Count(a => a.EndpointId == e.Id && a.State != AlertState.Resolved && a.HeldUntil != null && a.HeldUntil > now),
                Site = new MaintenancePeriod(e.Site!.MaintenanceStartedAt, e.Site.MaintenanceEndsAt, e.Site.MaintenanceStartedByName, e.Site.MaintenanceReason),
                Client = new MaintenancePeriod(e.Site.Client!.MaintenanceStartedAt, e.Site.Client.MaintenanceEndsAt, e.Site.Client.MaintenanceStartedByName,
                    e.Site.Client.MaintenanceReason)
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var windows = await MaintenanceWindowSchedule.RunningBySiteAsync(db, rows.Select(r => r.Endpoint.SiteId).Distinct().ToList(), now, cancellationToken);
        return rows.Select(r =>
        {
            var e = r.Endpoint;
            var maintenance = MaintenanceRules.Effective(e.Maintenance, r.Site, r.Client, now,
                MaintenanceWindowSchedule.PeriodFor(windows, e.SiteId, e.EffectiveClass));
            return new ApiEndpoint(e.Id, e.ClientId, e.SiteId, e.Hostname, Map(e.EffectiveClass), Map(e.DetectedClass), e.ClassOverride is not null,
                Map(e.Tier), Map(TierRules.EffectiveTier(e.Tier, license)), Map(e.Source), e.IsOnline, UtcOrNull(e.LastSeenAt),
                new ApiOperatingSystem(e.OsPlatform, e.OsName, e.OsVersion), e.Architecture, e.AgentVersion, Utc(e.EnrolledAt), e.PublicIpAddress,
                UtcOrNull(e.PublicIpSeenAt), r.OpenAlerts, r.HeldAlerts, maintenance is null ? null : Map(maintenance), Utc(e.CreatedAt), Utc(e.UpdatedAt));
        }).ToList();
    }

    /// <summary>Null when the endpoint does not exist; an endpoint without an inventory yet returns a found endpoint with a null value.</summary>
    public async Task<(bool EndpointFound, ApiInventory? Inventory)> GetInventoryAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        if (!await EndpointExistsAsync(caller, endpointId, cancellationToken))
        {
            return (false, null);
        }

        var inventory = await _endpoints.GetInventoryAsync(caller, endpointId, cancellationToken);
        if (inventory is null)
        {
            return (true, null);
        }

        var services = await _checks.GetServicesAsync(caller, endpointId, cancellationToken);
        return (true, new ApiInventory(endpointId, Utc(inventory.ReceivedAt), inventory.Manufacturer, inventory.Model, inventory.SerialNumber,
            new ApiCpu(inventory.CpuModel, inventory.CpuCores, inventory.CpuLogicalProcessors), inventory.MemoryTotalBytes, UtcOrNull(inventory.BootTime),
            inventory.Domain, inventory.LoggedOnUser,
            inventory.Disks.Select(d => new ApiDisk(d.Mount, d.Filesystem, d.TotalBytes, d.FreeBytes)).ToList(),
            inventory.NetworkInterfaces.Select(n => new ApiNetworkInterface(n.Name, n.MacAddress, n.IpAddresses)).ToList(),
            inventory.Software.Select(s => new ApiSoftware(s.Name, s.Version, s.Publisher, s.InstallDate)).ToList(),
            services.Select(s => new ApiService(s.Name, s.DisplayName, s.StartType, s.State)).ToList(),
            inventory.Action1AgentId));
    }

    /// <summary>Null when the endpoint does not exist.</summary>
    public async Task<ManagedRead<ApiEndpointChecks>?> GetChecksAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken)
    {
        var view = await _checks.GetChecksAsync(caller, endpointId, cancellationToken);
        if (view is null)
        {
            return null;
        }

        if (!view.Managed)
        {
            return new ManagedRead<ApiEndpointChecks>(false, null);
        }

        var items = view.Rows.Select(r => new ApiCheck(r.DefinitionId, r.Name, CheckTypeName(r.Type), r.Target,
                r.DisabledOnEndpoint ? ApiCheckStatus.Disabled
                : r.RerunRequested ? ApiCheckStatus.RerunRequested
                : r.Status is { } status ? Map(status)
                : ApiCheckStatus.NotRunYet,
                r.Value is { } value && double.IsFinite(value) ? value : null, r.Detail, r.Error, UtcOrNull(r.LastResultAt), r.IntervalSeconds, Map(r.Source), r.TemplateName, r.HasOverrides, r.OpenAlert?.Id,
                r.Parameters))
            .ToList();
        return new ManagedRead<ApiEndpointChecks>(true, new ApiEndpointChecks(endpointId, view.ConfigurationPending, items));
    }

    /// <summary>Newest first, keyset on (CreatedAt, Id). Null when the endpoint does not exist.</summary>
    public async Task<ManagedRead<ApiPage<ApiNote>>?> ListNotesAsync(Caller caller, Guid endpointId, string? cursor, int limit,
        CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId).Select(e => new { e.Tier }).SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        // Managed endpoints only, as in the UI (NoteService): notes of an agent-only endpoint stay stored but are not shown.
        if (TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return new ManagedRead<ApiPage<ApiNote>>(false, null);
        }

        var notes = db.Notes.AsNoTracking().Where(n => n.EndpointId == endpointId);
        if (cursor is not null && ApiCursor.Require(cursor, out DateTime createdAt, out var id))
        {
            notes = notes.Where(n => EF.Functions.LessThan(ValueTuple.Create(n.CreatedAt, n.Id), ValueTuple.Create(createdAt, id)));
        }

        var rows = await notes.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(limit + 1)
            .Select(n => new ApiNote(n.Id, n.EndpointId, n.AuthorName, n.Body, n.CreatedAt, n.EditedAt))
            .ToListAsync(cancellationToken);
        var page = Page(rows.Select(n => n with { CreatedAt = Utc(n.CreatedAt), EditedAt = UtcOrNull(n.EditedAt) }).ToList(), limit,
            n => ApiCursor.Encode(n.CreatedAt, n.Id));
        return new ManagedRead<ApiPage<ApiNote>>(true, page);
    }

    // Alerts

    /// <summary>Newest first, keyset on (OpenedAt, Id).</summary>
    public async Task<ApiPage<ApiAlert>> ListAlertsAsync(Caller caller, ApiAlertFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var now = _time.GetUtcNow().UtcDateTime;
        var alerts = db.Alerts.AsNoTracking();
        // Same meaning as the Alerts page (AlertService): open and acknowledged leave out alerts on hold.
        alerts = filter.State switch
        {
            AlertStateFilter.Open => alerts.Where(a => a.State == AlertState.Open && (a.HeldUntil == null || a.HeldUntil <= now)),
            AlertStateFilter.Acknowledged => alerts.Where(a => a.State == AlertState.Acknowledged && (a.HeldUntil == null || a.HeldUntil <= now)),
            AlertStateFilter.OnHold => alerts.Where(a => a.State != AlertState.Resolved && a.HeldUntil != null && a.HeldUntil > now),
            AlertStateFilter.Resolved => alerts.Where(a => a.State == AlertState.Resolved),
            _ => alerts
        };

        if (filter.Severity is { } severity)
        {
            alerts = alerts.Where(a => a.Severity == severity);
        }

        if (filter.ClientId is { } clientId)
        {
            alerts = alerts.Where(a => a.ClientId == clientId);
        }

        if (filter.EndpointId is { } endpointId)
        {
            alerts = alerts.Where(a => a.EndpointId == endpointId);
        }

        if (cursor is not null && ApiCursor.Require(cursor, out DateTime openedAt, out var id))
        {
            alerts = alerts.Where(a => EF.Functions.LessThan(ValueTuple.Create(a.OpenedAt, a.Id), ValueTuple.Create(openedAt, id)));
        }

        var rows = await alerts.OrderByDescending(a => a.OpenedAt).ThenByDescending(a => a.Id).Take(limit + 1).ToListAsync(cancellationToken);
        return Page(rows.Select(a => ToApi(a, now)).ToList(), limit, a => ApiCursor.Encode(a.OpenedAt, a.Id));
    }

    public async Task<ApiAlert?> GetAlertAsync(Caller caller, Guid alertId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        return alert is null ? null : ToApi(alert, _time.GetUtcNow().UtcDateTime);
    }

    private static ApiAlert ToApi(Alert a, DateTime now)
    {
        var held = a.State != AlertState.Resolved && a.HeldUntil is { } until && until > now;
        return new ApiAlert(a.Id, a.ClientId, a.EndpointId, Map(a.Kind), a.CheckDefinitionId, a.Target, Map(a.Severity), Map(a.State), held,
            held ? UtcOrNull(a.HeldUntil) : null, a.Title, a.Detail, Utc(a.OpenedAt), Utc(a.UpdatedAt), UtcOrNull(a.AcknowledgedAt), UtcOrNull(a.ResolvedAt),
            a.ResolvedReason);
    }

    // Jobs

    /// <summary>Newest first, keyset on (CreatedAt, Id). Jobs stay readable after the endpoint became agent-only: they are history.</summary>
    public async Task<ApiPage<ApiJob>> ListJobsAsync(Caller caller, ApiJobFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var jobs = db.Jobs.AsNoTracking();
        if (filter.ClientId is { } clientId)
        {
            jobs = jobs.Where(j => j.ClientId == clientId);
        }

        if (filter.EndpointId is { } endpointId)
        {
            jobs = jobs.Where(j => j.EndpointId == endpointId);
        }

        if (filter.State is { } state)
        {
            jobs = jobs.Where(j => j.State == state);
        }

        if (cursor is not null && ApiCursor.Require(cursor, out DateTime createdAt, out var id))
        {
            jobs = jobs.Where(j => EF.Functions.LessThan(ValueTuple.Create(j.CreatedAt, j.Id), ValueTuple.Create(createdAt, id)));
        }

        var rows = await ProjectJobs(jobs.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(limit + 1)).ToListAsync(cancellationToken);
        return Page(rows.Select(ToApi).ToList(), limit, j => ApiCursor.Encode(j.CreatedAt, j.Id));
    }

    public async Task<ApiJob?> GetJobAsync(Caller caller, Guid jobId, CancellationToken cancellationToken)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var row = await ProjectJobs(db.Jobs.AsNoTracking().Where(j => j.Id == jobId)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToApi(row);
    }

    /// <summary>The output as the UI shows it: the first megabyte per stream, decoded as UTF-8.</summary>
    public async Task<ApiJobOutput?> GetJobOutputAsync(Caller caller, Guid jobId, CancellationToken cancellationToken)
    {
        var view = await _jobs.GetOutputAsync(caller, jobId, cancellationToken);
        return view is null
            ? null
            : new ApiJobOutput(jobId, Map(view.Job.OutputState), view.Stdout, view.StdoutBytes, view.Stderr, view.StderrBytes, view.ShortenedInView,
                view.Job.OutputTruncated);
    }

    private sealed record JobRow(Guid Id, Guid BatchId, Guid ClientId, Guid EndpointId, JobType Type, Guid? ScriptId, Guid? ScriptVersionId, string ScriptName,
        int ScriptVersionNumber, ScriptLanguage Language, string ScriptSha256, JobRunAs RunAs, string? RunAsAccount, string? RunAsChosenAccount, JobState State, JobResult? Result, int? ExitCode, string? Problem,
        string InitiatedByName, DateTime CreatedAt, DateTime ValidUntil, DateTime? DeliveredAt, DateTime? StartedAt, DateTime? CompletedAt,
        JobOutputState OutputState, bool OutputTruncated, long ReceivedOutputBytes);

    // Payload, signature and output chunks are never selected: the list stays small and the signed payload never leaves the database.
    private static IQueryable<JobRow> ProjectJobs(IQueryable<Job> jobs) =>
        jobs.Select(j => new JobRow(j.Id, j.BatchId, j.ClientId, j.EndpointId, j.Type, j.ScriptId, j.ScriptVersionId, j.ScriptName, j.ScriptVersionNumber,
            j.Language, j.ScriptSha256, j.RunAs, j.RunAsAccount, j.RunAsChosenAccount, j.State, j.Result, j.ExitCode, j.RefusalReason ?? j.Error, j.InitiatedByName, j.CreatedAt, j.ValidUntil, j.DeliveredAt,
            j.StartedAt, j.CompletedAt, j.OutputState, j.OutputTruncated, j.ReceivedOutputBytes));

    private static ApiJob ToApi(JobRow j) =>
        new(j.Id, j.BatchId, j.ClientId, j.EndpointId, j.Type switch { JobType.Script => "script", _ => throw Unmapped(j.Type) },
            new ApiJobScript(j.ScriptId, j.ScriptVersionId, j.ScriptName, j.ScriptVersionNumber, Map(j.Language), j.ScriptSha256), Map(j.RunAs),
            j.RunAsAccount, j.RunAsChosenAccount, Map(j.State), j.Result is { } result ? Map(result) : null, j.ExitCode, j.Problem, j.InitiatedByName, Utc(j.CreatedAt), Utc(j.ValidUntil),
            UtcOrNull(j.DeliveredAt), UtcOrNull(j.StartedAt), UtcOrNull(j.CompletedAt), new ApiJobOutputSummary(Map(j.OutputState), j.OutputTruncated,
                j.ReceivedOutputBytes));

    // Shared

    private async Task<bool> EndpointExistsAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.Create(caller.Scope);
        return await db.Endpoints.AsNoTracking().AnyAsync(e => e.Id == endpointId, cancellationToken);
    }

    private static ApiPage<T> Page<T>(List<T> rows, int limit, Func<T, string> cursorOf)
    {
        if (rows.Count <= limit)
        {
            return new ApiPage<T>(rows, null);
        }

        rows.RemoveAt(limit);
        return new ApiPage<T>(rows, cursorOf(rows[^1]));
    }

    private static ApiMaintenance? Maintenance(MaintenancePeriod period, DateTime now) =>
        period.IsActive(now) ? new ApiMaintenance(Utc(period.StartedAt!.Value), UtcOrNull(period.EndsAt), period.StartedByName, period.Reason) : null;

    /// <summary>Timestamps leave the API as UTC with a Z, whatever kind the database driver returned.</summary>
    internal static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>snake_case name of a check type, e.g. <c>disk_free</c>. Check types are only ever appended, so names are stable.</summary>
    internal static string CheckTypeName(CheckType type) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(type.ToString());

    // Explicit maps: a new internal value must be added here on purpose (a test walks every value), never leak under its C# name.

    internal static ApiEndpointClass Map(EndpointClass value) => value switch
    {
        EndpointClass.Workstation => ApiEndpointClass.Workstation,
        EndpointClass.Server => ApiEndpointClass.Server,
        _ => throw Unmapped(value)
    };

    internal static ApiEndpointTier Map(EndpointTier value) => value switch
    {
        EndpointTier.AgentOnly => ApiEndpointTier.AgentOnly,
        EndpointTier.Managed => ApiEndpointTier.Managed,
        _ => throw Unmapped(value)
    };

    internal static ApiEndpointSource Map(EndpointSource value) => value switch
    {
        EndpointSource.Agent => ApiEndpointSource.Agent,
        EndpointSource.Integration => ApiEndpointSource.Integration,
        _ => throw Unmapped(value)
    };

    internal static ApiEndpointMaintenance Map(EffectiveMaintenance value) =>
        new(value.Source switch
            {
                MaintenanceSource.Endpoint => ApiMaintenanceSource.Endpoint,
                MaintenanceSource.Site => ApiMaintenanceSource.Site,
                MaintenanceSource.Client => ApiMaintenanceSource.Client,
                MaintenanceSource.PolicyWindow => ApiMaintenanceSource.PolicyWindow,
                _ => throw Unmapped(value.Source)
            }, Utc(value.StartedAt), UtcOrNull(value.EndsAt), value.StartedByName, value.Reason, value.SourceName);

    internal static ApiAlertKind Map(AlertKind value) => value switch
    {
        AlertKind.Check => ApiAlertKind.Check,
        AlertKind.Offline => ApiAlertKind.Offline,
        AlertKind.DuplicateIdentity => ApiAlertKind.DuplicateIdentity,
        AlertKind.AgentStopped => ApiAlertKind.AgentStopped,
        AlertKind.WatchdogStopped => ApiAlertKind.WatchdogStopped,
        _ => throw Unmapped(value)
    };

    internal static ApiAlertSeverity Map(AlertSeverity value) => value switch
    {
        AlertSeverity.Warning => ApiAlertSeverity.Warning,
        AlertSeverity.Critical => ApiAlertSeverity.Critical,
        _ => throw Unmapped(value)
    };

    internal static ApiAlertState Map(AlertState value) => value switch
    {
        AlertState.Open => ApiAlertState.Open,
        AlertState.Acknowledged => ApiAlertState.Acknowledged,
        AlertState.Resolved => ApiAlertState.Resolved,
        _ => throw Unmapped(value)
    };

    internal static ApiCheckStatus Map(CheckStatus value) => value switch
    {
        CheckStatus.Ok => ApiCheckStatus.Ok,
        CheckStatus.Warning => ApiCheckStatus.Warning,
        CheckStatus.Critical => ApiCheckStatus.Critical,
        CheckStatus.Unknown => ApiCheckStatus.Unknown,
        _ => throw Unmapped(value)
    };

    internal static ApiCheckSource Map(CheckSource value) => value switch
    {
        CheckSource.SiteTemplate => ApiCheckSource.SiteTemplate,
        CheckSource.EndpointTemplate => ApiCheckSource.EndpointTemplate,
        CheckSource.Endpoint => ApiCheckSource.Endpoint,
        _ => throw Unmapped(value)
    };

    internal static ApiJobState Map(JobState value) => value switch
    {
        JobState.PendingSignature => ApiJobState.PendingSignature,
        JobState.Queued => ApiJobState.Queued,
        JobState.Running => ApiJobState.Running,
        JobState.Succeeded => ApiJobState.Succeeded,
        JobState.Failed => ApiJobState.Failed,
        JobState.Expired => ApiJobState.Expired,
        JobState.Refused => ApiJobState.Refused,
        JobState.Lost => ApiJobState.Lost,
        JobState.Cancelled => ApiJobState.Cancelled,
        _ => throw Unmapped(value)
    };

    internal static ApiJobResult Map(JobResult value) => value switch
    {
        JobResult.Exited => ApiJobResult.Exited,
        JobResult.TimedOut => ApiJobResult.TimedOut,
        JobResult.Refused => ApiJobResult.Refused,
        JobResult.FailedToStart => ApiJobResult.FailedToStart,
        JobResult.Interrupted => ApiJobResult.Interrupted,
        _ => throw Unmapped(value)
    };

    internal static ApiJobRunAs Map(JobRunAs value) => value switch
    {
        JobRunAs.Service => ApiJobRunAs.Service,
        JobRunAs.LoggedOnUser => ApiJobRunAs.LoggedOnUser,
        _ => throw Unmapped(value)
    };

    internal static ApiJobOutputState Map(JobOutputState value) => value switch
    {
        JobOutputState.None => ApiJobOutputState.None,
        JobOutputState.Receiving => ApiJobOutputState.Receiving,
        JobOutputState.Complete => ApiJobOutputState.Complete,
        JobOutputState.Incomplete => ApiJobOutputState.Incomplete,
        _ => throw Unmapped(value)
    };

    internal static ApiScriptLanguage Map(ScriptLanguage value) => value switch
    {
        ScriptLanguage.PowerShell => ApiScriptLanguage.PowerShell,
        ScriptLanguage.Batch => ApiScriptLanguage.Batch,
        ScriptLanguage.Shell => ApiScriptLanguage.Shell,
        ScriptLanguage.Bash => ApiScriptLanguage.Bash,
        _ => throw Unmapped(value)
    };

    private static ArgumentOutOfRangeException Unmapped<T>(T value) where T : struct, Enum =>
        new(nameof(value), value, $"{typeof(T).Name}.{value} has no public API value yet. Add it to PublicApiQueries and MD-Files/API.md.");
}
