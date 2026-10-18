using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>A period of the page, anchored on the server clock.</summary>
public enum PerfPeriod
{
    Minutes15,
    Hour1,
    Hours6,
    Hours24,
    Days7
}

/// <summary>Settings, Performance as one view: the period, its scores, findings, timeline and the dataset behind them.</summary>
public sealed record PerformanceView(
    PerfPeriod Period,
    DateTime From,
    DateTime To,
    TimeSpan Bucket,
    PerformanceSettings Settings,
    IReadOnlyList<PerfCategoryScore> Scores,
    IReadOnlyList<PerfFinding> Findings,
    IReadOnlyList<PerfTimelinePoint> Timeline,
    PerformanceDataset Data,
    bool CanManage);

/// <summary>
/// The performance monitor in web (0.7.0, PERFORMANCE.md §8): what the four components measured, judged by
/// <see cref="PerformanceRules"/>, for users with the permission Performance, which counts only for users whose groups give every
/// client. Changing the settings needs Manage performance monitoring and is audited with the old and new values; every export is
/// audited. What leaves the server is redacted, the client codes and endpoint host names of the instance included.
/// </summary>
public sealed class PerformanceService
{
    public const int SettingsRowId = 1;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly PerfSettingsCache _cache;
    private readonly TimeProvider _time;

    public PerformanceService(IFleetoDbContextFactory dbFactory, INotificationBus bus, PerfSettingsCache cache, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _cache = cache;
        _time = time;
    }

    public static TimeSpan Length(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 => TimeSpan.FromMinutes(15),
        PerfPeriod.Hour1 => TimeSpan.FromHours(1),
        PerfPeriod.Hours6 => TimeSpan.FromHours(6),
        PerfPeriod.Hours24 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(7)
    };

    public static TimeSpan BucketOf(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 or PerfPeriod.Hour1 => TimeSpan.FromMinutes(1),
        PerfPeriod.Hours6 => TimeSpan.FromMinutes(5),
        PerfPeriod.Hours24 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };

    public static string Label(PerfPeriod period) => period switch
    {
        PerfPeriod.Minutes15 => "15 minutes",
        PerfPeriod.Hour1 => "1 hour",
        PerfPeriod.Hours6 => "6 hours",
        PerfPeriod.Hours24 => "24 hours",
        _ => "7 days"
    };

    public async Task<PerformanceView?> GetAsync(Caller caller, PerfPeriod period, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return null;
        }

        var settings = await ReadSettingsAsync(cancellationToken);
        var (from, to, bucket) = Range(period);
        await using var db = _dbFactory.CreateSystem();
        var data = await PerformanceReader.ReadAsync(db, from, to, settings, cancellationToken);
        var findings = PerformanceRules.Evaluate(data);
        var timeline = await PerformanceReader.ReadTimelineAsync(db, from, to, bucket, cancellationToken);
        return new PerformanceView(period, from, to, bucket, settings, PerformanceRules.Score(data, findings), findings, timeline, data,
            caller.Has(Permissions.PerformanceManage));
    }

    public async Task<PerformanceSettings?> GetSettingsAsync(Caller caller, CancellationToken cancellationToken = default) =>
        caller.Has(Permissions.PerformanceView) ? await ReadSettingsAsync(cancellationToken) : null;

    /// <summary>Validates and stores the settings, audits what changed and tells every component.</summary>
    public async Task<ServiceResult> SaveSettingsAsync(Caller caller, PerformanceSettings settings, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceManage))
        {
            return ServiceResult.Forbidden();
        }

        // Only thresholds that differ from their default are stored, so a default changed in a later release reaches every instance.
        settings = settings with
        {
            DisabledCollectors = settings.DisabledCollectors.Distinct().Order().ToList(),
            Thresholds = settings.Thresholds
                .Where(t => PerfThresholds.Find(t.Key) is not { } info || Math.Abs(info.Default - t.Value) > 1e-9)
                .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal)
        };
        if (settings.Problem() is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.PerformanceSettings.SingleOrDefaultAsync(s => s.Id == SettingsRowId, cancellationToken);
        var before = PerformanceSettings.Parse(row?.Json);
        if (row is null)
        {
            row = new PerformanceSettingsRow { Id = SettingsRowId };
            db.PerformanceSettings.Add(row);
        }

        row.Json = settings.Serialize();
        row.UpdatedAt = now;
        row.UpdatedBy = caller.UserId;
        var details = new
        {
            Before = JsonSerializer.Deserialize<JsonElement>(before.Serialize(), AuditJson),
            After = JsonSerializer.Deserialize<JsonElement>(row.Json, AuditJson)
        };
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceSettingsChanged, "PerformanceSettings",
            SettingsRowId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, details), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _cache.Set(settings);
        try
        {
            await _bus.PublishAsync(NotificationChannels.Performance, string.Empty, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The other components read the settings again within 30 seconds anyway.
        }

        return ServiceResult.Ok();
    }

    /// <summary>The report for Claude Code as Markdown, and an audit entry for the export (<paramref name="how"/>: "copied" or "downloaded").</summary>
    public async Task<ServiceResult<string>> ExportMarkdownAsync(Caller caller, PerfPeriod period, string how, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.PerformanceView))
        {
            return ServiceResult<string>.Forbidden();
        }

        var settings = await ReadSettingsAsync(cancellationToken);
        var (from, to, _) = Range(period);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var data = await PerformanceReader.ReadAsync(db, from, to, settings, cancellationToken);
        var findings = PerformanceRules.Evaluate(data);
        var redactor = await RedactorAsync(db, cancellationToken);
        var report = PerformanceReport.Markdown(data, findings, PerformanceRules.Score(data, findings), now, FleetoVersion.Current, redactor);

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PerformanceExported, "Instance", instance.InstanceId.ToString(), null,
            new { How = how, Format = "markdown", Period = Label(period), Bytes = System.Text.Encoding.UTF8.GetByteCount(report) }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<string>.Ok(report);
    }

    /// <summary>The redactor of this instance: its client codes and endpoint host names are masked wherever they slip in.</summary>
    private static async Task<PerfRedactor> RedactorAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var codes = await db.Clients.AsNoTracking().Select(c => c.Code).ToListAsync(cancellationToken);
        var hosts = await db.Endpoints.AsNoTracking().Select(e => e.Hostname).Where(h => h != "").ToListAsync(cancellationToken);
        return PerfRedactor.ForInstance(codes, hosts);
    }

    private async Task<PerformanceSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var json = await db.PerformanceSettings.AsNoTracking().Where(s => s.Id == SettingsRowId).Select(s => s.Json).FirstOrDefaultAsync(cancellationToken);
        return PerformanceSettings.Parse(json);
    }

    /// <summary>The period ending at the current minute of the server clock, so buckets line up with the minute rows.</summary>
    private (DateTime From, DateTime To, TimeSpan Bucket) Range(PerfPeriod period)
    {
        var to = PerfRecorder.MinuteOf(_time.GetUtcNow().UtcDateTime).AddMinutes(1);
        return (to - Length(period), to, BucketOf(period));
    }
}
